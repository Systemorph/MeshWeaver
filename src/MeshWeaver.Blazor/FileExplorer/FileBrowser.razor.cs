using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.FileExplorer;

/// <summary>
/// Blazor component that displays and manages files and folders within a named content collection.
/// Supports browsing, uploading, downloading, creating folders, and deleting items.
/// </summary>
public partial class FileBrowser
{
    [Inject] private IDialogService DialogService { get; set; } = null!;
    private IContentService ContentService => Hub.ServiceProvider.GetRequiredService<IContentService>();
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    /// <summary>Name of the content collection to browse. Changing this value reloads the collection.</summary>
    [Parameter] public string? CollectionName { get; set; } = "";
    /// <summary>Current folder path within the collection. Defaults to the root.</summary>
    [Parameter] public string? CurrentPath { get; set; } = "/";
    /// <summary>The highest-level path the browser allows navigating to; prevents navigation above this folder.</summary>
    [Parameter] public string TopLevelPath { get; set; } = "";
    /// <summary>When true the browser renders in an embedded (compact) mode without a full-page chrome.</summary>
    [Parameter] public bool Embed { get; set; }
    /// <summary>When true, automatically creates <c>CurrentPath</c> inside the collection on initialization if it does not exist.</summary>
    [Parameter] public bool CreatePath { get; set; }
    /// <summary>When true, shows the "New Article" action in the toolbar.</summary>
    [Parameter] public bool ShowNewArticle { get; set; }
    /// <summary>Optional configuration registered with the content service on initialization, creating the collection if it does not already exist.</summary>
    [Parameter] public ContentCollectionConfig? CollectionConfiguration { get; set; }
    /// <summary>Mesh address used to build download and preview URLs for files in the collection.</summary>
    [Parameter] public Address? Address { get; set; }
    /// <summary>When true, hides upload, create, and delete controls so the browser is read-only.</summary>
    [Parameter] public bool IsReadOnly { get; set; }
    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];
    FluentInputFile myFileByStream = default!;
    /// <summary>
    /// Runs after each parameter update: registers any new collection configuration, loads the collection by name,
    /// optionally creates <c>CurrentPath</c>, and refreshes the displayed items.
    /// </summary>
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

        var items = new List<CollectionItem>();
        await foreach (var item in Collection.GetCollectionItems(CurrentPath!))
            items.Add(item);
        CollectionItems = items;
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
            // Documents with converter support are previewed, not downloaded
            ".docx" => false,
            // Other binary formats remain download-only
            ".xlsx" or ".xls" or ".doc" or ".pptx" or ".ppt" or ".zip" => true,
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

                // Post-upload seam: raise the SAME observer the MCP upload path raises
                // (MeshOperations.Upload → hub.RaiseContentUploaded) so a GUI upload
                // auto-indexes like any other upload. Fire-and-forget by contract — the
                // indexing runs as its own Activity and never blocks the upload (#170).
                var relativePath = string.IsNullOrEmpty(CurrentPath)
                    ? file.Name
                    : $"{CurrentPath.Trim('/')}/{file.Name}".TrimStart('/');
                Hub.RaiseContentUploaded(QualifiedCollectionPath, relativePath);

                ToastService.ShowSuccess($"File {file.Name} successfully uploaded.");
            }
        }
        catch (Exception e)
        {
            ToastService.ShowError($"Error uploading {file.Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The node-qualified collection path (<c>{address}/{collection}</c>) that upload
    /// observers expect — the same shape <c>MeshOperations.Upload</c> raises and the same
    /// composition <see cref="GetLink(FileItem)"/> uses for download URLs. Falls back to
    /// <see cref="CollectionName"/> as-is when no <see cref="Address"/> is set or the name
    /// is already qualified with it.
    /// </summary>
    private string QualifiedCollectionPath
    {
        get
        {
            var address = Address?.ToString();
            return string.IsNullOrEmpty(address)
                   || CollectionName!.StartsWith(address + "/", StringComparison.OrdinalIgnoreCase)
                ? CollectionName!
                : $"{address}/{CollectionName}";
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
